# Plan: server profiles, auto-activated by the multiplayer server

Status: proposed.

## Context
Some commands only make sense on one server, e.g. its own F10 comms menu items. Today `common.json` is merged
under every aircraft profile (`ProfileStore.LoadMerged`), so server commands would be active everywhere.

Goal: a **server profile**, a common-style profile linked to one or more servers. It turns on by itself when you
join a matching server, and off when you leave.

DCS-BIOS can't tell us the server. A DCS hook can. Tested as a client on a real server
(`tools/dcs-hook-probe/`):

| Info | Call | Example |
|---|---|---|
| Server name | `net.get_server_settings().name` | `" ShadowReapers Dynamic Campaign Modern PvE "` (trim it) |
| Server address | `net.get_server_host()` | `65.109.30.115:10321` |
| Mission | `DCS.getMissionName()` | `SRDCModernCaucasus` |
| Map | `DCS.getCurrentMission().mission.theatre` | `Caucasus` |
| Multiplayer | `DCS.isMultiplayer()` | `true` |

Single player needs nothing new: `common.json` covers it.

## Approach

### 1. Hook: `DcsVoiceCom.GameGUI.lua` (new, in `src/DcsVoiceCom.App/Assets/` or `installer/`)
- Goes into `Saved Games\DCS\Scripts\Hooks\`.
- Uses **only calls the probe showed safe as a client**. Never `DCS.getTheatreID()`: it crashed DCS before the map loaded.
- Sends one small JSON line over UDP to `127.0.0.1:7790` (setting):
  `{"v":1,"event":"mission","mp":true,"server":"ShadowReapers ...","address":"65.109.30.115:10321","mission":"SRDCModernCaucasus","map":"Caucasus"}`
- When: `onSimulationStart`, our own `onPlayerChangeSlot`, and every 10 s during the mission (throttled in
  `onSimulationFrame`), so the app also picks it up if it starts after DCS.
- Sends `{"v":1,"event":"stop"}` on `onSimulationStop` and `onNetDisconnect`.
- Sends only the fields above. `get_server_settings()` also holds a password hash; the hook never sends or logs it.
- Every call in `pcall`; a failing hook must not stop the mission.

### 2. App: `DcsHookListener` (new, `src/DcsVoiceCom.Core/DcsHook/`)
- Listens on `127.0.0.1:7790`, local only.
- Parses the line into `DcsSession { Multiplayer, Server, Address, Mission, Map }` and raises `SessionChanged`
  when it changes. No message for 30 s means the session ended (DCS crashed or quit).

### 3. Server profiles
- Same JSON format as `common.json`, plus a list of servers it applies to:
  ```json
  { "aircraft": "server-ShadowReapers", "servers": ["ShadowReapers*"], "commands": [ ... ] }
  ```
- Stored as `server-<name>.json` in the profiles folder (`server-` prefix, like `common`).
- `servers` entries match the server name, ignoring case and outer spaces. `*` is a wildcard, so one profile can
  cover all ShadowReapers servers. An entry can also be an address (`65.109.30.115:10321`).
- `ProfileStore.List()` leaves server profiles out of the aircraft list, like `common`.

### 4. Merging (`ProfileStore.LoadMerged`)
- Order: **aircraft > server profiles > common**. A command with the same name in a higher layer wins, as today.
- More than one server profile can match; they're merged by file name order.
- `VoiceComRuntime` reloads the profile on `SessionChanged`, the same way it does on `AircraftChanged`.

### 5. UI
- Main window: show `Server: <name>` and the active server profile(s) next to the aircraft.
- Profile list: server profiles are editable like `common`. New "New server profile…" fills `servers` with the
  current server name.
- Settings: "Install DCS hook" button copies the hook to `Saved Games\DCS\Scripts\Hooks\` (and
  `DCS.openbeta` if present), with status "installed / not installed / out of date".
- Help: update `docs/help/` (profiles page and settings page), and `docs/profiles.md`.

### 6. Tests
- `DcsHookListener`: parsing, change events, timeout.
- Server name matching: trim, case, wildcard, address.
- Merge order: aircraft > server > common.
- Hook Lua: run under a fake `DCS`/`net` (as done for the probe) to check messages and that the hash is never sent.

## Open questions
1. Match on server **name** (wildcard), address, or both? Proposed: both, name first. Names can change; addresses
   can be shared by several servers (ShadowReapers runs 4 on one IP).
2. Should the app install the hook itself (Settings button), or the installer? Proposed: Settings button, so it
   also works for zip users.
3. Pin a server profile by hand (like the aircraft pin), for testing offline? Proposed: yes, in the profile menu.
