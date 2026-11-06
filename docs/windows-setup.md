# Running DCS VoiceCom on Windows

The easy way: run `DcsVoiceCom-Setup-<version>.exe` (see the README for how to build it). It installs the app, the
bundled model and the sample profiles, then you only need the Settings step below. The rest of this page builds the
app from the checkout instead; speech runs inside the app, so there is nothing else to install.

## Once

You need:

- the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0);
- DCS-BIOS (Skunkworks) installed in DCS as usual. The app reads its control list from
  `%USERPROFILE%\Saved Games\DCS\Scripts\DCS-BIOS\doc\json`.

In PowerShell, from the `dcs-voicecom` checkout:

```powershell
# 1. Sample profiles (optional: you can also build profiles in the app)
New-Item -ItemType Directory -Force "$env:LOCALAPPDATA\DcsVoiceCom\profiles" | Out-Null
Copy-Item profiles\*.json "$env:LOCALAPPDATA\DcsVoiceCom\profiles\"

# 2. Build the app into dist\DcsVoiceCom, then start it
dotnet publish src\DcsVoiceCom.App -c Release -r win-x64 --self-contained false -o dist\DcsVoiceCom
.\dist\DcsVoiceCom\DcsVoiceCom.exe
```

`dist\DcsVoiceCom\DcsVoiceCom.exe` is the app; pin it to the taskbar or make a shortcut. Run the publish line again
after pulling changes. (`dotnet run -c Release --project src\DcsVoiceCom.App` also works; it builds into
`src\DcsVoiceCom.App\bin\Release\net10.0-windows10.0.19041.0\`, not into a `bin` folder at the top.)

`dotnet run` and the exe print nothing in the console: the app is a window. If it fails to start, it shows a message
and writes `%LOCALAPPDATA%\DcsVoiceCom\crash.log`.

The Whistle model and the needle engine are in the repository (`model\cactus-needle`), and the build copies them
next to the exe, so speech works offline from the first start. The first start writes
`%LOCALAPPDATA%\DcsVoiceCom\settings.json`; the log names the model folder it used. The app looks for the model in
this order:

1. the model folder set in Settings;
2. `model\cactus-needle` next to the exe (the bundled copy);
3. `%LOCALAPPDATA%\DcsVoiceCom\model`, where the app downloads it;
4. `model\cactus-needle` in a folder above the exe (the checkout), then WhistleAttack's `model\cactus-needle` beside
   it, then `%USERPROFILE%\.cache\cactus-needle`.

In folders 2–4 the app only uses files whose SHA-256 matches the pinned Whistle 2.0.0 weights and needle 3.3.3
engine, so a library dropped into one of them is ignored. A folder set in Settings is used as is.

Only if none has it does the app download the Whistle weights (17 MB) and the engine (1.5 MB) from Hugging Face into
`%LOCALAPPDATA%\DcsVoiceCom\model`, checking their SHA-256. Then open **Settings** once for:

- **Push-to-talk → Rebind**, then press the HOTAS button within 15 s.
- **Microphone**, if it isn't the Windows default input.

Save. The voice engine restarts, and the status bar should show *Speech · ready*.

### Without the app: settings by hand

The console host (`dotnet run -c Release --project src\DcsVoiceCom.Host`, with `-- --init`, `-- --bind` and
`-- --fetch-model`) uses the same settings file. Speech settings live under `speech`; use your own paths, with doubled
backslashes:

```json
"speech": {
  "device": "Headset Microphone",
  "modelDirectory": "C:\\src\\whistleattack\\model\\cactus-needle"
}
```

Leave `modelDirectory` out (or `null`) to use the bundled model. `language` at the top selects `en` or `de`.
A settings file from the Python sidecar days keeps its `device` and `modelDirectory`; the rest of its `sidecar`
section is dropped.

### All settings

`settings.json`, all paths configurable:

| Key | Default |
|---|---|
| `language` | `en` (`de` for German; Whistle also knows `fr es it nl pl`) |
| `dcsBiosJsonPath` | `%USERPROFILE%\Saved Games\DCS\Scripts\DCS-BIOS\doc\json` |
| `profilesPath` | `%LOCALAPPDATA%\DcsVoiceCom\profiles` |
| `speech.device` | microphone, part of its name; empty: the Windows default input |
| `speech.modelDirectory` | Whistle model folder laid out like the cactus-needle cache (`whistle\<ver>\whistle.cact`, `v3\<ver>\libneedle.dll`); empty: the bundled `model\cactus-needle` next to the exe (see the search order above), else downloaded |
| `speech.preRollMs` / `phraseKeywords` / `margin` / `autoDownload` | `200`, `true`, `10`, `true` |
| `ptt.deviceGuid` / `deviceName` / `button` | set by `--bind` |
| `dcsBios.allowRemoteExport` | `false`: export packets from other PCs are dropped; set `true` when DCS runs on another PC |
| `keyHoldMs` | `50` |
| `cues.accepted` / `noMatch` / `refused` | bundled WAVs |
| `profileOverride` | `null` (Auto) |

### DCS on another PC

By default the app only accepts DCS-BIOS export packets sent from this PC, so nobody else on the network can fake
the cockpit state. If DCS runs on a different PC, turn that check off in `settings.json` (there is no switch in the
Settings window):

```json
"dcsBios": {
  "allowRemoteExport": true
}
```

Restart the app after the change. Also set `dcsBios.inputHost` to that PC's address so commands reach it.

## Each time

Start `dist\DcsVoiceCom\DcsVoiceCom.exe` (or `dotnet run -c Release --project src\DcsVoiceCom.App`),
then DCS and the F-14B (Upgraded). The status bar shows the aircraft, and the matching profile gets a green dot.
Hold the PTT button, say "gear up", release. The log shows a line like
`HEAR “Gear up.” → gear up (100) → Gear up` and the DCS-BIOS lines it sent.

In the log panel you can type a phrase and press Enter to run it without speaking; with *Dry run* ticked, it only
lists the steps. The profile drop-down in the top bar pins a profile; *Auto* follows the aircraft again. Minimizing
hides the window to the tray icon, whose menu does the same.

The console host still works for the same things: type a phrase, `:dry PHRASE`, `:profile NAME|auto`, `:reload`, `:quit`.

## When something is off

| Log says | Do |
|---|---|
| no window at all | read `%LOCALAPPDATA%\DcsVoiceCom\crash.log`; also check the tray, where a minimized window goes |
| `speech unavailable: ... SHA-256` or a download error | the bundled `model` folder next to the exe is missing, and the download failed; publish again, or set a model folder in Settings |
| `speech unavailable: no whistle\<version>\whistle.cact ...` | the model folder in Settings doesn't hold the model; clear it to use the download |
| `speech unavailable: no microphone matches '...'` | pick the microphone again in Settings; the message lists the ones Windows has active |
| `speech unavailable: Windows has no default microphone` | plug one in, or set one as the default recording device, or pick one in Settings |
| `No push-to-talk button bound` | Settings → Rebind (console host: `--bind`; `--devices` lists the controllers DirectInput sees) |
| no aircraft / profile line, with DCS on another PC | set `dcsBios.allowRemoteExport` to `true` (see [DCS on another PC](#dcs-on-another-pc)) |
| no aircraft / profile line | DCS-BIOS is not running in DCS, or the profile file name does not match the aircraft name the log prints |
| `NoMatch ...: too close to call` or `... not heard` | the matcher would not choose between two commands, or a word of the phrase was missing; say it again, or make the phrases differ more |

Speech messages (model, microphone, what was heard) appear in the log tagged SPEECH.
