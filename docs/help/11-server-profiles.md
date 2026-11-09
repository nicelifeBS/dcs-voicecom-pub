---
title: Server profiles
group: Commands
---
# Server profiles

Some commands only make sense on one multiplayer server, like its own F10 comms menu items. Put them in a
**server profile**. It turns on by itself when you join a matching server, and off when you leave.

1. Settings → **DCS hook** → **Install**. Restart DCS if it's running. The hook tells the app which server
   you're on.
2. Join the server. Below the profile list it says **Server · *name***.
3. Click **+ Server profile**. It starts with the server's name. Add commands like in any profile.

**Servers**, above the command list, lists the servers the profile is for, one per line. `*` stands for any text:

| Line | Matches |
|---|---|
| `ShadowReapers*` | names that start with *ShadowReapers* |
| `*Reapers*` | names that contain *Reapers* |
| `*PvE` | names that end with *PvE* |
| `65.109.30.115:10321` | that server address |

Without `*` the whole name must match. Case doesn't matter.

Commands are merged in this order: aircraft, then server profile, then *common*. If two have a command with
the same name, the first one wins.

> **Tip:** Test without DCS: pick the server profile in the drop-down below the profile list to pin it. Pick
> *Auto* to follow the server again.
