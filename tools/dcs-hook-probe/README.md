# DCS hook probe

A test hook. It checks which mission, map and server info a DCS hook can read,
so we can decide if the app needs its own hook. See if it works as a client on a
multiplayer server, not only in single player.

## Use

1. Copy `DcsVoiceComProbe.lua` to `Saved Games\DCS\Scripts\Hooks\`.
2. Start DCS and do these tests:
   - a single-player mission,
   - join a multiplayer server and pick a slot,
   - if you can, host a server yourself.
3. Open `Saved Games\DCS\Logs\dcs-voicecom-probe.log` and share it.
4. Delete the hook file when you're done.

The hook only writes to the log. It never sends anything or changes the game.

## What to look for

- `mission.theatre`: the map name, e.g. `Caucasus`.
- `DCS.getMissionName()` / `mission.sortie`: the mission name.
- `briefing ...`: the briefing text (description and each side's task).
- `net ... functions`: every `net` call DCS offers. Look for one with the server name.
- `get_player_info` / `get_server_host`: may hold the server name or address.
