---
name: run-dcs-voicecom
description: Build, run, drive and screenshot the DCS VoiceCom WPF app (DcsVoiceCom.exe) on Linux under Wine + Xvfb. Use when asked to run, start, launch, click through, test the UI of, or take a screenshot of the app, its main window, Settings or Seed dialog, or to check a XAML/layout change visually.
---

# Run DCS VoiceCom (WPF under Wine)

The app is a WPF `net10.0-windows` exe. Here it is published self-contained for `win-x64` and run under Wine
on a headless Xvfb display. Drive it with `.claude/skills/run-dcs-voicecom/driver.sh`: it clicks, types and
sends keys with `xdotool`, and takes screenshots with ImageMagick `import`. Paths below are relative to the
repo root. All state lives in `/tmp/dcs-voicecom-run` (override with `DCSVC_WORK`).

## Prerequisites

`wine` (9.0, 64-bit only), `xvfb`, `xdotool`, `imagemagick`, and the Liberation, JetBrains Mono and DejaVu fonts.
Most containers lack them; `setup` installs the missing ones with apt, and pip-installs `fonttools`.
It also deletes a Wine prefix that a failed run left half made and builds a new one. To retake the help
screenshots, use the `help-screenshots` skill.

## Setup and build (about 1 minute, once per container)

```bash
D=.claude/skills/run-dcs-voicecom/driver.sh
$D setup     # .NET 10 SDK (Ubuntu dotnet-sdk-10.0) if missing, Wine prefix, fonts, sample profiles
$D build     # dotnet publish src/DcsVoiceCom.App -r win-x64 --self-contained -> /tmp/dcs-voicecom-run/pub
```

Re-run `build` after every code or XAML change, then `quit` and `launch`.

## Run (agent path)

```bash
D=.claude/skills/run-dcs-voicecom/driver.sh
$D launch                  # waits for the "DCS VoiceCom" window and moves it to 0,0 (1432x866)
$D click 120 104           # select the F-14 profile (first row of the profile list)
$D click 400 180           # select its first command -> the editor fills in
$D ss main                 # -> /tmp/dcs-voicecom-run/shots/main.png (main window only)
$D click 600 651           # Log panel test box
$D type "landing gear up"
$D key Return              # runs the phrase through the matcher; the box keeps its text, so key ctrl+a before the next one
$D click 1315 36           # Settings button (Help is at 1389 36, or key F1)
$D waitwin '^Settings$'    # dialog ignores clicks for ~2s after it appears
$D windows                 # lists windows: "Settings" at 400,70 632x726
$D sswin '^Settings$' settings-dialog
$D click 868 761           # Cancel (screen coordinates)
$D wait 2                  # the dialog takes 1-2s to go away
$D ssroot full             # whole 1600x1000 display, including dialogs and popups
$D quit
```

Read the PNG after each `ss`. Coordinates are screen pixels. The main window sits at 0,0, so they match
window pixels. Dialogs open at their own offset; use `windows` to find it. To find a control, take a
screenshot and read its position off the image.

Run `driver.sh` with no arguments to print all commands (`rclick`, `dclick` and `log` among them).

## Run (human path)

On Windows, run `dotnet run --project src/DcsVoiceCom.App`. This was not verified here.

## Test

```bash
dotnet test     # Core and ViewModels tests; the App project fails with MSB4019 under Ubuntu's SDK (expected)
```

## What you will and won't see

- **Real:** the layout, theme, bindings, profile and command lists, the editor, the Settings dialog, and the Log panel.
- **Offline:** DCS-BIOS shows "waiting for DCS" and speech shows "offline": Wine has no microphone, so the log shows
  `speech unavailable: Windows has no default microphone`. Typed tests still go through the real matcher.
- **Whistle loads for real:** the build bundles `model\cactus-needle` next to the exe, and the log shows
  `Whistle loaded in ... ms`.
- **Empty control browser** (right column, "No DCS-BIOS definitions for this profile"). It needs the JSON files
  that DCS-BIOS writes inside DCS, which aren't in this repo. To fill it, copy some to
  `/tmp/dcs-voicecom-run/wine/drive_c/users/root/Saved Games/DCS/Scripts/DCS-BIOS/doc/json/`.
- **Fonts are stand-ins:** Liberation Sans is renamed to "Segoe UI" and JetBrains Mono to "Cascadia Mono".
  Metrics are close but not identical to Windows.
- **No window manager:** dialogs have no title bar or frame. To close a dialog, click its buttons; don't rely on
  `key Escape`, which did not close Settings in one run.

## Gotchas

- **`launch` can hold the calling shell open.** A Wine process inherits its output, so a tool call that pipes
  `launch` (`| tail`) never returns, and killing it kills the app. Detach it:
  `setsid nohup $D launch > launch.log 2>&1 < /dev/null &`, then wait for `window` in `launch.log`.
- **A ComboBox dropdown crashed Wine once** (`X Error ... RENDER ... RenderCreatePicture`, app gone). To pin a profile
  without the dropdown, set `"profileOverride": "F-14"` in the prefix's `settings.json` before `launch`.
- **SDK:** Ubuntu's `dotnet-sdk-10.0` (in `/usr/lib/dotnet`) includes the WPF `Microsoft.NET.Sdk.WindowsDesktop`
  targets, so `setup` only installs it when missing. (Ubuntu's 8.0 SDK lacked them, MSB4019.) Set `DCSVC_SDK` to
  use another SDK folder. The Windows runtime packs come from nuget.org through a normal restore.
- **Use `wine`, not `wine64`.** There is no `wine64` on PATH in Ubuntu's wine 9.0. It prints "wine32 is
  missing", which is harmless for this x64 app.
- **WPF FailFasts on text without fonts:** `Process terminated. Unrecoverable system error.` at
  `TypefaceMap.MapUnresolvedCharacters`. Copying TTFs into `C:\windows\Fonts` is not enough. Wine's DirectWrite
  only sees fonts listed under `HKLM\Software\Microsoft\Windows NT\CurrentVersion\Fonts`, and WPF looks fonts up
  by family name, so the files are renamed with fonttools and then registered with `regedit`.
- **Wine is slow to react.** A dialog closes 1-2 s after its Cancel click, so a screenshot taken straight away
  still shows it, with Cancel focused. A new dialog drops clicks for about 2 s. Use `waitwin` after opening a
  dialog and `wait 2` before a screenshot that has to show the result.
- **The window opens centered** at 84,80. `launch` moves it to 0,0 so coordinates are stable.
- **`ss` uses `import -window <id>`.** It captures that one X window. Popups such as ComboBox dropdowns and
  context menus are separate windows, so use `ssroot` for them.
- **`log` shows Wine's stdout, not the in-app log.** It is only useful for crash traces. The app's own log is
  the Log panel on screen.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `MSB4019 ... Microsoft.NET.Sdk.WindowsDesktop.targets was not found` | You built with `/usr/bin/dotnet`. Use `driver.sh build`. |
| `launch` prints `Process terminated` + `MapUnresolvedCharacters` | The font step is missing. Re-run `driver.sh setup`. |
| `launch` says `already running` but nothing is visible | A previous run is still alive. Run `driver.sh quit` and then `launch`. |
| Clicks land in the wrong place | The window wasn't moved. Check that `driver.sh windows` shows `0,0`. If not, `quit` and `launch` again. |
| Lists are empty, "No profile selected" | The profiles weren't seeded. `setup` copies `profiles/*.json` into the prefix's `AppData/Local/DcsVoiceCom/profiles`. |
