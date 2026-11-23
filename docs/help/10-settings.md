---
title: Settings
group: Reference
---
# Settings

![The Settings window](images/settings.png)

- **Push-to-talk.** *Rebind*, then press a HOTAS button within 15 seconds. One button for every aircraft.
- **Speech.** Microphone, language, speech engine and the Whistle model folder. Leave the folder empty to use the
  built-in model.
  - **Whistle** (default) runs every language. Small and fast.
  - **Zipformer** is English only and better at odd words like "Jester", "AWACS" or "Shkval". It downloads once
    (about 75 MB) and uses about 150 MB more memory. For another language the app uses Whistle.
  - **Word boost** (Zipformer only, default 1.5): how strongly it leans towards your profile's words. Higher can
    help an odd word, but can also turn other speech into a command word.
- **Matching.** The score a command needs (85) and a critical command needs (95), 50 to 100. For every profile.
- **DCS-BIOS connection.** Host, ports and the folder with the aircraft definitions. The defaults fit a normal
  install.
- **DCS hook.** *Install* puts a small script into `Saved Games\DCS\Scripts\Hooks`. It tells the app which
  multiplayer server you're on, for [server profiles](11-server-profiles.md). *Remove* takes it out again.
- **Profiles and keys.** Where the profile files are kept, how long a key is held, and the sounds for accepted,
  no match and refused.
- **Debug.** *Save the audio of every command* writes each push-to-talk clip to
  `%LOCALAPPDATA%\DcsVoiceCom\debugging` as a `.wav` with a `.txt` note (result, what was heard, mic level). Misses
  start with `Result: MISS` and the reason. Play the clip to tell a bad microphone from weak recognition. Off by
  default; the newest 200 are kept.

Saving restarts the voice engine. All settings are in `%LOCALAPPDATA%\DcsVoiceCom\settings.json`.
