---
name: help-screenshots
description: Retake the in-app help screenshots (docs/help/images) of DCS VoiceCom under Wine + Xvfb. Use when asked to retake, refresh or update the help screenshots or images, or at release time after a UI change, once the user said yes.
---

# Retake the help screenshots

To check whether a release needs new screenshots, see the UI changes since the last screenshot commit:

```bash
git diff --stat $(git log -1 --format=%h -- docs/help/images) -- src/DcsVoiceCom.App
```

This runs the real app under Wine through `.claude/skills/run-dcs-voicecom/driver.sh` (see that skill for driving
the app by hand). From the repo root:

```bash
.claude/skills/run-dcs-voicecom/driver.sh setup
```

```bash
tools/help-screenshots/take.sh
```

`setup` takes about 1 minute in a fresh container and a few seconds after that, so always run it first. It:

- installs `wine`, `xvfb`, `xdotool`, `imagemagick` and the fonts with apt when they are missing (most cloud
  containers have none of them),
- installs the .NET 10 SDK when it is missing,
- deletes a Wine prefix that a failed run left half made, then builds a new one.

`take.sh` builds the app, takes every shot, draws the markers and writes `docs/help/images/*.png`.
It takes a few minutes; give the tool call a 10-minute timeout.

## Then

1. Read every PNG in `docs/help/images/` that changed. If a click missed, fix the coordinates in `take.sh`
   and run it again.
2. The numbered markers in `main-window.png` must match the list in `docs/help/02-main-window.md`.
3. `git status`: images that only differ by log timestamps or other noise can be reverted with `git checkout`.
4. Commit the images with the release.

## When it fails

| Symptom | Cause and fix |
|---|---|
| `wine: command not found`, or `no window after 90s` | `setup` was skipped. Run it. |
| `FileNotFoundError ... windows/Fonts/segoe-Regular.ttf` | Half-made Wine prefix from an old driver. `driver.sh quit`, `rm -rf /tmp/dcs-voicecom-run/wine`, then `setup`. |
| `launch` shows `c0000135` or `Process terminated` | Same broken prefix, or the font step is missing. Same fix. |
| `apt-get` can't find a package | Run `apt-get update`, then `setup` again. |
| A shot is all black | Wine was repainting. Run `take.sh --no-build` again. |
