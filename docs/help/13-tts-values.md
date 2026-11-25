---
title: TTS values
group: Commands
---
# TTS values

A *Say* step can speak a live cockpit value, like the clock or the throttle: `Time is {clock}`. The raw value is
often hard to say (`203422`, `52428`), so you set it up once as a **value**. Every command in the profile can use it.

Open it with **TTS** in the main window, or **Insert value…** in the Say window.

1. **+ New** and give it a name, like `clock`. Use it in Say text as `{clock}`.
2. **Source.** The DCS-BIOS output to read. Type words to search.
3. **Mode.** *Slice* or *Remap*.
4. Check the **Preview** and press **Play**.

## Slice: pick characters

Click a character, Shift+click to widen, then press **Add**. Type anything else you want between the pieces.

| Template | Sample | Says |
|---|---|---|
| `{0-1}:{2-3}:{4-5}` | `203422` | 20:34:22 |
| `{2-5}` | `  8450` | 8450 |

*Pad with zeros* keeps numbers the same length: `509` padded to 6 is `000509`.

## Remap: scale a number

For levers and knobs, like a throttle that goes from 0 to 65535. Remap it to 0-100 and it says `80` for `52428`.
*Cockpit value* is filled in from DCS-BIOS. Values outside the range are clamped. *Decimals* sets 0, 1 or 2.

## Good to know

- Without DCS, type a sample value to set it up. The value is read live when the command runs.
- No DCS data when the command runs? The Say step is skipped, and the Log says why.
- A spoken slot of the same name wins inside its command. The window warns about it.
- **Rename** updates the Say steps that use it. **Delete** warns when commands still use it.
- Changes are saved with the profile (**Save**).

> **Tip:** The voice reads the text as it is. "20:34:22" may sound different per voice: try **Play**.
