---
title: Profile file format
group: Reference
---
# Profile file format

A profile is a JSON file. You can edit it in a text editor, share it, or keep it in Git. Files are in
`%LOCALAPPDATA%\DcsVoiceCom\profiles\`. After editing by hand, press **F5** in the app to reload.

| File | For |
|---|---|
| `F-14.json` | One aircraft (named like its DCS-BIOS control file) |
| `F-14BU.json` | One variant only; found before the shared file |
| `common.json` | All aircraft |
| `server-<name>.json` | A multiplayer server (see [Server profiles](11-server-profiles.md)) |

## Example

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

## Profile

| Field | Meaning |
|---|---|
| `aircraft` | Aircraft name, `common`, or `server-…` |
| `servers` | Server profiles only: names or addresses, `*` = any text |
| `keywords` | Extra words the recognizer gets wrong |
| `module_version` | DCS-BIOS version the controls were checked against |
| `commands` | The list of commands |

## Command

| Field | Meaning |
|---|---|
| `name` | Name of the command. Must be unique |
| `description` | Your own note |
| `phrases` | Voice lines (see [Writing phrases](03-phrases.md)) |
| `steps` | What to do, in order |
| `critical` | `true` = needs the critical threshold from Settings |
| `enabled` | `false` = ignored |
| `seeded` | Set by seeding; cleared when you edit the command |

## Steps

Each step has a `type`:

| `type` | What it does | Main fields |
|---|---|---|
| `bios` | Sends one DCS-BIOS line. With `press_ms` it is a button press | `control`, `arg`, `skip_if_already`, `press_ms`, `release` |
| `bios_until` | Turns a knob until a value is reached | `control`, `target`, `output`, `scale`, `tolerance`, `inc`, `dec`, `max_steps`, `delay_ms` |
| `key` | Presses a key chord, like `RCtrl+G`. DCS must have focus | `keys`, `hold_ms` |
| `wait` | Waits | `ms` |
| `wait_for` | Waits until a condition (see Conditions below) is true. On timeout the command is refused | `until`, `timeout_ms` |
| `if` | Runs `then` or `else` depending on a condition (see Conditions below) | `condition`, `then`, `else` |
| `cue` | Plays a sound: `accepted`, `no_match` or `refused` | `cue` |
| `say` | Speaks a voice reply, like `Radio set to {freq}` (see [Actions and steps](04-actions.md)). Doesn't wait for the speech | `text` |
| `foreach_char` | Repeats `steps` for each character of `value`, with `{char}` set. `map` renames characters (`"."` to `"DOT"`) | `value`, `map`, `steps` |
| `enter_value` | Puts a spoken value into a radio or keypad (see [Enter value](06-enter-value.md)) | `value`, `method`, `display`, `fields` |

### bios or bios_until?

`bios` sends one line and moves on. `bios_until` turns a knob step by step (`INC` or `DEC`) and reads the cockpit
after every step, until the value reaches your target.

| | `bios` | `bios_until` |
|---|---|---|
| Sends | One line: a value, `INC`, `DEC`, `TOGGLE` or `+3200` | `INC` or `DEC` again and again |
| Reads the cockpit | No (only `skip_if_already` checks once) | Yes, after every step |
| Use it for | Switches, levers, buttons | Knobs with no direct value, like a heading knob |
| Needs a target | No | Yes, can be a spoken value like `{deg}` |
| Stops when | The line is sent | The value is within `tolerance` of `target × scale`, or it overshoots. After `max_steps` the command is refused |

For "gear up", use `bios` and send `0`. For "set heading {deg}", use `bios_until`.

### Conditions

`wait_for` and `if` test a cockpit output: `{ "output": "…", "op": "=", "value": "…" }`. `op` can be `=`, `!=`,
`<`, `<=`, `>`, `>=` (default `=`). Numbers compare as numbers. Text only works with `=` and `!=`.

### Placeholders

A spoken value (see [Writing phrases](03-phrases.md)) can be used in `control`, `arg`, `target`, `keys`, `value`
and `text` as `{name}`. Inside `foreach_char`, `{char}` is the current character.

## Rules

- Match thresholds are in Settings, not in the file.
- Names use `snake_case`; case doesn't matter when reading.
- `//` comments and trailing commas are allowed.
- Unknown fields are ignored. Left-out fields use their default.
- A step with a missing or unknown `type` stops the file from loading.
- Other mistakes (like a bad slot name) are listed in the log as *Profile problem*, and the phrase is skipped.

> **Tip:** Build commands in the app first, then open the file to see how they are stored.
