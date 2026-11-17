---
title: Settings
group: Reference
---
# Settings

![The Settings window](images/settings.png)

- **Push-to-talk.** *Rebind*, then press a HOTAS button within 15 seconds. One button for every aircraft.
- **Speech.** Microphone, language and the Whistle model folder. Leave the folder empty to use the built-in model.
- **DCS-BIOS connection.** Host, ports and the folder with the aircraft definitions. The defaults fit a normal
  install.
- **DCS hook.** *Install* puts a small script into `Saved Games\DCS\Scripts\Hooks`. It tells the app which
  multiplayer server you're on, for [server profiles](11-server-profiles.md). *Remove* takes it out again.
- **Profiles and keys.** Where the profile files are kept, how long a key is held, and the sounds for accepted,
  no match and refused.
- **Debug.** *Save the audio of missed commands* writes each miss to `%LOCALAPPDATA%\DcsVoiceCom\debugging` as a
  `.wav` with a `.txt` note (what was heard, why it missed, mic level). Play the clip to tell a bad microphone
  from weak recognition. Off by default; the newest 200 are kept.

Saving restarts the voice engine. All settings are in `%LOCALAPPDATA%\DcsVoiceCom\settings.json`.
