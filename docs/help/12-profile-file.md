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

| `type` | Main fields |
|---|---|
| `bios` | `control`, `arg`, `skip_if_already`, `press_ms`, `release` |
| `bios_until` | `control`, `target`, `output`, `scale`, `tolerance`, `inc`, `dec`, `max_steps`, `delay_ms` |
| `key` | `keys`, `hold_ms` |
| `wait` | `ms` |
| `wait_for` | `until`, `timeout_ms` |
| `if` | `condition`, `then`, `else` |
| `cue` | `cue`: `accepted`, `no_match` or `refused` |
| `foreach_char` | `value`, `map`, `steps` |
| `enter_value` | `value`, `method`, `display`, `fields` (see [Enter value](06-enter-value.md)) |

A condition is `{ "output": "…", "op": "=", "value": "…" }`. `op` can be `=`, `!=`, `<`, `<=`, `>`, `>=`.

## Rules

- Match thresholds are in Settings, not in the file.
- Names use `snake_case`; case doesn't matter when reading.
- `//` comments and trailing commas are allowed.
- Unknown fields are ignored. Left-out fields use their default.
- A step with a missing or unknown `type` stops the file from loading.
- Other mistakes (like a bad slot name) are listed in the log as *Profile problem*, and the phrase is skipped.

> **Tip:** Build commands in the app first, then open the file to see how they are stored.
