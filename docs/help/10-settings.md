---
title: Settings
group: Reference
---
# Settings

![The Settings window](images/settings.png)

- **Push-to-talk.** *Rebind*, then press a HOTAS button within 15 seconds. One button for every aircraft.
- **Speech.** Microphone, language and the Whistle model folder. Leave the folder empty to use the built-in model.
- **Matching.** The score a command needs (85) and a critical command needs (95), 50 to 100. For every profile.
- **DCS-BIOS connection.** Host, ports and the folder with the aircraft definitions. The defaults fit a normal
  install.
- **DCS hook.** *Install* puts a small script into `Saved Games\DCS\Scripts\Hooks`. It tells the app which
  multiplayer server you're on, for [server profiles](11-server-profiles.md). *Remove* takes it out again.
- **Profiles and keys.** Where the profile files are kept, how long a key is held, and the sounds for accepted,
  no match and refused.
- **Debug: speed up speech.** Runs speech at above-normal priority while it recognises a command, and stops
  Windows from slowing the app down to save power. On by default. Turn it off if DCS stutters when you speak.
- **Debug: log CPU use.** Logs the CPU model and cores at start, then for each command how busy the CPU was and how
  long speech took (`[cpu]` lines). Off by default.
- **Debug: save audio.** Writes each push-to-talk clip to `%LOCALAPPDATA%\DcsVoiceCom\debugging` as a `.wav` with
  a `.txt` note (result, what was heard, mic level). Misses start with `Result: MISS` and the reason. Play the clip
  to tell a bad microphone from weak recognition. Off by default; the newest 200 are kept.

Saving restarts the voice engine. All settings are in `%LOCALAPPDATA%\DcsVoiceCom\settings.json`.
